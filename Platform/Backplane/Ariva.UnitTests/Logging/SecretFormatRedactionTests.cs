using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Ariva.Api.Common.Hosting;
using Ariva.Api.Common.Logging;
using Ariva.Infra.Integration;
using Ariva.Infra.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace Ariva.UnitTests.Logging;

/// <summary>
/// ARV-212 (CWE-532, ASVS 5.0 V16.2.5): every credential Ariva issues or stores is masked inside log text, by its shape or by
/// the name it is written under, and so is the text of a logged exception. Each credential is drawn from Ariva's own
/// generator, many times, so the rules are proven on what Ariva issues, not on a sample written to fit them. Rows name no
/// credential (the TRX keeps a row's arguments, ARV-252): each test draws its own.
/// </summary>
public sealed class SecretFormatRedactionTests
{
    private const int Draws = 2_000;
    private static readonly string Redacted = RedactionEnricher.Redacted;

    /// <summary>The ways a credential reaches a log line as text: bare, in a sentence, quoted, as a value, at the start and at the end.</summary>
    private static IEnumerable<string> Around(string credential) =>
    [
        credential,
        $"call refused with {credential} at 10:30",
        $"\"{credential}\"",
        $"value={credential};next=1",
        $"{credential}: rejected",
        $"rejected ({credential})"
    ];

    private static bool Holds(string text, string secret) => text.Contains(secret, StringComparison.Ordinal);

    #region Shapes

    [Fact]
    public void RedactText_Should_MaskTheIntegrationClientSecret_When_ItAppearsInText()
    {
        for (var i = 0; i < Draws; i++)
        {
            var secret = IntegrationCredentials.NewSecret();
            foreach (var text in Around(secret))
            {
                var redacted = RedactionEnricher.RedactText(text);
                Holds(redacted, secret[IntegrationCredentials.SecretMarker.Length..]).Should().BeFalse("the secret is masked");
                redacted.Should().Contain(IntegrationCredentials.SecretMarker + Redacted, "the marker stays, so the line says what was masked");
            }
        }
    }

    [Fact]
    public void RedactText_Should_MaskTheTemporaryPassword_When_ItAppearsInText()
    {
        var policy = new PasswordPolicy(["ariva", "AMM"]);
        for (var i = 0; i < Draws; i++)
        {
            var password = TemporaryPasswords.New(policy, "officer.one");
            foreach (var text in Around(password))
            {
                var redacted = RedactionEnricher.RedactText(text);
                Holds(redacted, password).Should().BeFalse("a temporary password is masked whole");
                password.Split('-').Any(group => Holds(redacted, group)).Should().BeFalse("no group of it is left either");
            }
        }
    }

    [Fact]
    public void RedactText_Should_MaskTheRecoveryCode_When_ItAppearsInText()
    {
        for (var i = 0; i < Draws; i++)
        {
            var code = RecoveryCodes.New();
            foreach (var text in Around(code))
                Holds(RedactionEnricher.RedactText(text), code).Should().BeFalse("a recovery code as Ariva shows it is masked");
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(32)]
    [InlineData(80)]
    public void RedactText_Should_MaskTheTotpSeed_When_ItAppearsInTextOrAnAuthenticatorAddress(int seedBytes)
    {
        // 20 bytes is a user's and an integration client's seed (32 characters); 10 and 80 bytes are the outbound endpoint's
        // shortest and longest (16 and 128 characters).
        for (var i = 0; i < Draws; i++)
        {
            var bytes = RandomNumberGenerator.GetBytes(seedBytes);
            var seed = Base32.Encode(bytes);
            foreach (var text in Around(seed))
                Holds(RedactionEnricher.RedactText(text), seed).Should().BeFalse("a base32 seed is masked");
            Holds(RedactionEnricher.RedactText(Totp.OtpAuthUri("Ariva", "officer.one", bytes)), seed).Should().BeFalse("the seed is masked in the authenticator's address");
        }
    }

    [Fact]
    public void RedactText_Should_MaskTheOutboundClientCertificate_When_ItsPkcs12IsInText()
    {
        // An outbound endpoint's mTLS client certificate is stored as its PKCS#12 in base64 (OutboundSecret.CertificatePfxBase64).
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=aodb-client.example", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var pfx = Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, "kept-out"));

        foreach (var text in Around(pfx))
            Holds(RedactionEnricher.RedactText(text), pfx[3..40]).Should().BeFalse("the PKCS#12 is masked");
    }

    [Fact]
    public void RedactText_Should_MaskEveryPrivateKeyBlock_When_APemKeyIsInText()
    {
        // The token signing keys (EC), the Data Protection certificate's key (RSA) and any key a site gives in PEM, encrypted or not.
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rsa = RSA.Create(2048);
        var parameters = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
        string[] blocks =
        [
            ec.ExportPkcs8PrivateKeyPem(), ec.ExportECPrivateKeyPem(), rsa.ExportRSAPrivateKeyPem(), rsa.ExportPkcs8PrivateKeyPem(),
            rsa.ExportEncryptedPkcs8PrivateKeyPem("kept-out", parameters)
        ];

        foreach (var block in blocks)
        {
            var body = block.Split('\n').Where(line => line.Length > 0 && !line.StartsWith('-')).ToList();
            var redacted = RedactionEnricher.RedactText($"key file read:\n{block}\nthen refused");
            body.Any(line => Holds(redacted, line)).Should().BeFalse("no line of the key's body is left");
            redacted.Should().EndWith("then refused", "the text after the key block stays");
        }
    }

    #endregion

    #region Names

    [Fact]
    public void RedactText_Should_MaskTheOutboundSecrets_When_TheyAreWrittenAsJsonMembers()
    {
        // The outbound token exchange body and the secret request's members, each value drawn at random.
        var values = Enumerable.Range(0, 6).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(12))).ToArray();
        var json = $$"""{"clientId":"aodb-1","clientSecret":"{{values[0]}}","apiKey":"{{values[1]}}","hmacKey":"{{values[2]}}","totpSeed":"{{values[3]}}","certificatePassword":"{{values[4]}}","password" : "{{values[5]}}","totpCode":123456}""";

        var redacted = RedactionEnricher.RedactText($"outbound call failed: {json}");

        values.Any(value => Holds(redacted, value)).Should().BeFalse("every secret member's value is masked");
        Holds(redacted, "123456").Should().BeFalse("a TOTP code written as a number is masked too");
        redacted.Should().Contain("\"clientId\":\"aodb-1\"", "a member with a harmless name keeps its value");
        redacted.Should().Contain("\"clientSecret\":\"" + Redacted + "\"");
    }

    [Fact]
    public void RedactText_Should_MaskTheSecretValues_When_TheyAreWrittenAsNameValuePairs()
    {
        var values = Enumerable.Range(0, 7).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(12))).ToArray();
        string[] texts =
        [
            $"grant_type=client_credentials&client_id=aodb-1&client_secret={values[0]}&scope=flights",
            $"Host=db;Database=ariva;Username=ariva_runtime;Password={values[1]};SSL Mode=VerifyFull",
            $"Host=db;Pwd={values[2]}",
            $"redis:6379,password={values[3]},ssl=true",
            $"sasl.username=ariva sasl.password={values[4]}",
            $"cookie __Secure-ariva_rt={values[5]}; path=/api/auth",
            $"Authorization=Basic%20{values[6]}"
        ];

        foreach (var text in texts)
        {
            var redacted = RedactionEnricher.RedactText(text);
            values.Any(value => Holds(redacted, value)).Should().BeFalse("the secret value is masked (text {0})", Array.IndexOf(texts, text));
        }

        RedactionEnricher.RedactText(texts[0]).Should().Contain("client_id=aodb-1").And.Contain("scope=flights", "the other pairs stay");
        RedactionEnricher.RedactText(texts[1]).Should().Contain("Username=ariva_runtime").And.Contain("Database=ariva");
    }

    [Fact]
    public void RedactText_Should_MaskTheWholePassword_When_AConnectionStringBuilderQuotedIt()
    {
        // NpgsqlConnectionStringBuilder quotes a value holding '=', ';', a quote or white space: in double quotes (a double
        // quote doubled), or in single quotes when the value holds a double quote. Base64 with padding is quoted too.
        var base64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        string[] passwords = [base64, $"a;b={token}", $"q\"{token}\"q", $"x y'{token}", $"d\"{token}'s"];

        foreach (var password in passwords)
        {
            var built = new Npgsql.NpgsqlConnectionStringBuilder { Host = "db", Database = "ariva", Username = "ariva_runtime", Password = password }.ConnectionString;
            var redacted = RedactionEnricher.RedactText($"database open failed: {built}");

            Holds(redacted, password).Should().BeFalse("the password is masked whole (password {0})", Array.IndexOf(passwords, password));
            Holds(redacted, token).Should().BeFalse("no part of it is left (password {0})", Array.IndexOf(passwords, password));
            Holds(redacted, "Username=ariva_runtime").Should().BeTrue("the other settings stay");
        }
    }

    [Fact]
    public void RedactText_Should_MaskTheSecret_When_APairSitsInTheQueryOfAnotherPairsAddress()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

        var redacted = RedactionEnricher.RedactText($"redirect next=https://idp.example/cb?client_secret={secret}&state=ok refused");

        Holds(redacted, secret).Should().BeFalse("a pair in an address's query is a pair of its own");
        Holds(redacted, "state=ok").Should().BeTrue();
        Holds(RedactionEnricher.RedactText($"Password=ab#{secret}"), secret).Should().BeFalse("a '#' does not end an unquoted value");
    }

    [Fact]
    public void RedactText_Should_MaskThePairsInsideAQuotedValue_When_ItsOwnNameIsHarmless()
    {
        var values = Enumerable.Range(0, 5).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(12))).ToArray();
        string[] texts =
        [
            $"ConnectionStrings=\"Host=db;Password={values[0]}\"",
            $"msg=\"retry with password={values[1]}\" at 10:30",
            $"x='client_secret={values[2]}'",
            $"msg=\"connection refused, Pwd={values[3]}",
            $"declare -x Redis__ConnectionString=\"redis:6379,password={values[4]},ssl=true\""
        ];

        foreach (var text in texts)
            values.Any(value => Holds(RedactionEnricher.RedactText(text), value)).Should().BeFalse("a pair inside a quoted value is masked (text {0})", Array.IndexOf(texts, text));
    }

    [Fact]
    public void RedactText_Should_MaskThePasswordToTheNextSemicolon_When_ItHoldsASeparatorOfAnotherPair()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        string[] passwords = [$"ab?{token}", $"ab,{token}", $"ab&{token}", $"ab<{token}>"];

        foreach (var password in passwords)
        {
            var redacted = RedactionEnricher.RedactText($"Host=db;Password={password};Database=ariva");
            Holds(redacted, token).Should().BeFalse("no part of the password is left (password {0})", Array.IndexOf(passwords, password));
            Holds(redacted, "Database=ariva").Should().BeTrue("the next setting stays");
        }
    }

    [Fact]
    public void RedactText_Should_MaskABareKey_When_AnAuthorizationValueHasNoKnownScheme()
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        var redacted = RedactionEnricher.RedactText($"Authorization: {key} sent twice");

        Holds(redacted, key).Should().BeFalse("only a known scheme word is kept");
        RedactionEnricher.RedactText("Authorization: negotiate abc").Should().Be("Authorization: negotiate " + Redacted);
    }

    [Theory]
    [InlineData("MIA")]
    [InlineData("MIG")]
    [InlineData("MII")]
    [InlineData("MIJ")]
    [InlineData("MIM")]
    [InlineData("MIP")]
    public void RedactText_Should_MaskADerStructure_When_ItsLengthTakesAnyLongForm(string start)
    {
        // A SEQUENCE with an indefinite length (MIA to MID), or one length byte (MIG, MIH), two (MII to MIL) or three (MIM to MIP).
        var der = start + Convert.ToBase64String(RandomNumberGenerator.GetBytes(120));

        Holds(RedactionEnricher.RedactText($"pfx {der} refused"), der[3..]).Should().BeFalse();
    }

    [Theory]
    [InlineData("a=")]
    [InlineData("a=b")]
    [InlineData("\"a\":\"")]
    [InlineData("Cookie:")]
    [InlineData("ABCDEFGH")]
    [InlineData("A-")]
    [InlineData("eyJ-")]
    [InlineData("eyJabcde.")]
    [InlineData("Bearer ")]
    [InlineData("?token=")]
    [InlineData("ardk_-")]
    // ARV-289 (checkpoint fix): the near misses of the two backtracking rules, an attempt failing at each start: a name with no
    // '=', and a marker with 19 key characters before a character no key holds.
    [InlineData("?token")]
    [InlineData("&access_tokex=")]
    [InlineData("ardk_0123456789012345678.")]
    public void RedactText_Should_FinishQuickly_When_TheTextIsALongAdversarialRun(string fragment)
    {
        // A rule's own fragments up to the most the rules read of one text (ARV-293: a longer one is cut first), with a private
        // key block left open first.
        var marker = new string('-', 5);
        var opening = marker + "BEGIN PRIVATE KEY" + marker;
        var text = opening + string.Concat(Enumerable.Repeat(fragment, (SecretText.MaxProtectedLength - opening.Length) / fragment.Length));
        // ARV-289 review: the linear engine's first long match in a process builds its states (about a second on a cold host for
        // the token parameter and device key rules, linear still), so the first pass is untimed; a quadratic rule is slow in either.
        RedactionEnricher.RedactText(text);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        RedactionEnricher.RedactText(text);
        RedactionEnricher.RedactText(text[opening.Length..]);

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "every rule is linear in the text's length");
    }

    [Fact]
    public void RedactText_Should_MaskTheCredentialHeaders_When_AHeaderDumpIsLoggedAsText()
    {
        var values = Enumerable.Range(0, 5).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(12))).ToArray();
        var dump = string.Join("\n",
            "Accept: application/json",
            $"Cookie: __Secure-ariva_rt={values[0]}; theme=dark",
            $"X-Api-Key: {values[1]}",
            $"Authorization: Digest username=\"x\", response=\"{values[2]}\"",
            $"x-ariva-csrf:{values[3]}",
            $"Authorization: {values[4]}");

        var redacted = RedactionEnricher.RedactText(dump);

        values.Any(value => Holds(redacted, value)).Should().BeFalse("every credential header's value is masked");
        redacted.Should().Contain("Accept: application/json").And.Contain("Authorization: Digest " + Redacted, "the scheme of an Authorization value stays");
        redacted.Split('\n').Should().HaveCount(6, "each header keeps its line");
    }

    [Theory]
    [InlineData("TotpSeed")]
    [InlineData("HmacKey")]
    [InlineData("CertificatePfxBase64")]
    [InlineData("Pwd")]
    [InlineData("__Secure-ariva_rt")]
    [InlineData("__Secure-ariva_jobs_hub")]
    public void IsSensitive_Should_BeTrue_When_ANameHoldsAnOutboundSecretOrTheRefreshCookie(string name)
    {
        RedactionEnricher.IsSensitive(name).Should().BeTrue();
    }

    #endregion

    #region What stays

    /// <summary>Text that looks a little like a credential but is not one: every rule leaves it as it is.</summary>
    public static TheoryData<string> Harmless => new()
    {
        "zone ARR-Q1 queue 12 at 10:30, flight EK123 landed",
        "track-event and check-tests ran, proxy_cache_bypass kept",
        "logics_data received from 00:6E:02:08:92:64",
        "client_id=ic_abc&scope=flights&state=ready",
        "thumbprint 0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C4B5A69788796A5B4C3D2E1F0",
        "request 6f9619ff-8b86-d011-b42d-00c04fc964ff finished in 12 ms",
        "AUTHENTICATIONS refused: 15 capitals are no seed",
        "Error: timeout after 30 s, https://ariva.example/api/health",
        "{\"zone\":\"ARR-Q1\",\"length\":12,\"headers\":\"none\"}",
        "MIIsmall and ics_short and ABCDE-FGHJ"
    };

    [Theory]
    [MemberData(nameof(Harmless))]
    public void RedactText_Should_LeaveTheTextAsItIs_When_ItHoldsNoCredential(string text)
    {
        RedactionEnricher.RedactText(text).Should().BeSameAs(text);
    }

    #endregion

    #region Rules

    // ARV-289 (checkpoint fix): every rule but the token parameter's and the device key's, which run on the backtracking engine
    // without a timeout (LinearRuleTests.SecretTextRule_Should_RunOnTheBacktrackingEngineWithoutATimeout_When_ItIsTheTokenOrDeviceKeyRule),
    // linear by their shape; their long runs are timed below with the others.
    public static TheoryData<string> RuleNames =>
        ["AuthorizationScheme", "Jwt", "PrivateKeyBlock", "DerBase64", "IntegrationClientSecret", "TemporaryPassword", "RecoveryCode",
         "Base32Seed", "CredentialHeader", "JsonMember", "Assignment", "UnquotedAssignment", "PasswordAssignment"];

    [Theory]
    [MemberData(nameof(RuleNames))]
    public void Rule_Should_BeLinearWithoutATimeout_When_TheEnricherRunsIt(string name)
    {
        // ARV-163: linear in the text's length; no timeout, which would let the event out unprotected (Serilog emits an event
        // whose enricher threw).
        var rule = (Regex)typeof(RedactionEnricher).GetMethod(name)?.Invoke(null, null);

        rule.Options.Should().HaveFlag(RegexOptions.NonBacktracking);
        rule.MatchTimeout.Should().Be(Regex.InfiniteMatchTimeout);
    }

    #endregion

    #region Exceptions

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

#pragma warning disable CA1064 // a test double, never thrown outside this class
    private sealed class UnreadableException : Exception
#pragma warning restore CA1064
    {
        public UnreadableException()
        {
        }

        public UnreadableException(string message)
            : base(message)
        {
        }

        public UnreadableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public override string ToString() => throw new InvalidOperationException("no text");
    }

    private static string Rendered(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        new JsonFormatter(renderMessage: true).Format(logEvent, writer);
        return writer.ToString();
    }

    private static Exception Thrown(Func<Exception> make)
    {
        try
        {
            throw make();
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [Fact]
    public void Sink_Should_WriteTheExceptionWithoutItsCredentials_When_ItsMessageOrAnInnerOneHoldsThem()
    {
        var secret = IntegrationCredentials.NewSecret();
        var code = RecoveryCodes.New();
        var inner = Thrown(() => new InvalidOperationException($"code {code} refused for anna.salem@example.com"));
        var exception = Thrown(() => new HttpRequestException($"token exchange failed with {secret}", inner));
        var sink = new ListSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(new ProtectedExceptionSink(sink)).CreateLogger();

        logger.Error(exception, "Outbound call {Endpoint} failed", "aodb-1");

        var written = sink.Events.Should().ContainSingle().Subject;
        var rendered = Rendered(written);
        Holds(rendered, secret[4..]).Should().BeFalse("the secret in the message is masked");
        Holds(rendered, code).Should().BeFalse("the code in the inner exception is masked");
        Holds(rendered, "anna.salem@example.com").Should().BeFalse("an address is masked as in any other text");
        rendered.Should().Contain("System.Net.Http.HttpRequestException", "the text still names the original type")
            .And.Contain("System.InvalidOperationException").And.Contain(nameof(Thrown), "the stack stays");
        written.Exception.Message.Should().Contain("ics_" + Redacted);
        written.Properties["Endpoint"].ToString().Should().Be("\"aodb-1\"");
    }

    [Fact]
    public void Sink_Should_PassTheExceptionAsItIs_When_ItsTextHoldsNoCredential()
    {
        var exception = Thrown(() => new TimeoutException("the database did not answer in 30 s"));
        var sink = new ListSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(new ProtectedExceptionSink(sink)).CreateLogger();

        logger.Warning(exception, "Retrying");

        sink.Events.Should().ContainSingle().Which.Exception.Should().BeSameAs(exception, "nothing to protect, so the original goes on");
    }

    [Fact]
    public void Sink_Should_LeaveTheTextOut_When_TheExceptionsTextCannotBeRead()
    {
        var sink = new ListSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(new ProtectedExceptionSink(sink)).CreateLogger();

        logger.Error(new UnreadableException(), "Failed");

        var written = sink.Events.Should().ContainSingle().Subject;
        written.Exception.Should().BeOfType<ProtectedException>();
        written.Exception.ToString().Should().Be(ProtectedExceptionSink.Unprotectable, "it fails closed and the event still goes out");
    }

    [Fact]
    public void Sink_Should_KeepTheStackAndInnerExceptionsForLoki_When_ItProtectsAnException()
    {
        // Loki's formatter writes the exception's type, message, stack and inner exceptions from their properties, not its
        // ToString: each must be the protected original's.
        var secret = IntegrationCredentials.NewSecret();
        var code = RecoveryCodes.New();
        var inner = Thrown(() => new InvalidOperationException($"code {code} refused"));
        var exception = Thrown(() => new HttpRequestException($"token exchange failed with {secret}", inner));
        var sink = new ListSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(new ProtectedExceptionSink(sink)).CreateLogger();

        logger.Error(exception, "Outbound call failed");

        using var writer = new StringWriter();
        new Serilog.Sinks.Grafana.Loki.LokiJsonTextFormatter().Format(sink.Events.Should().ContainSingle().Subject, writer);
        var loki = writer.ToString();
        Holds(loki, secret[4..]).Should().BeFalse("the secret is masked in Loki's line");
        Holds(loki, code).Should().BeFalse("and in its inner exception");
        Holds(loki, nameof(Thrown)).Should().BeTrue("the stack is written");
        Holds(loki, "System.Net.Http.HttpRequestException").Should().BeTrue("the original type opens the message");
        Holds(loki, "System.InvalidOperationException").Should().BeTrue("the inner exception is written, with its original type");
    }

    [Fact]
    public void Sink_Should_KeepTheTraceAndSpan_When_ItProtectsAnException()
    {
        using var activity = new System.Diagnostics.Activity("arv-212").Start();
        var sink = new ListSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(new ProtectedExceptionSink(sink)).CreateLogger();

        logger.Error(Thrown(() => new InvalidOperationException($"client {IntegrationCredentials.NewSecret()} refused")), "Failed");

        var written = sink.Events.Should().ContainSingle().Subject;
        written.Exception.Should().BeOfType<ProtectedException>();
        written.TraceId.Should().Be(activity.TraceId, "the line still belongs to its trace");
        written.SpanId.Should().Be(activity.SpanId);
    }

    [Fact]
    public void Sink_Should_CountTheFailure_When_AnExceptionsTextCannotBeRead()
    {
        using var metrics = new SecurityEventMetrics();
        using var reader = new Ariva.UnitTests.Setup.InstrumentReader(metrics.Meter);
        var sink = new ListSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(new ProtectedExceptionSink(sink, metrics)).CreateLogger();

        logger.Error(new UnreadableException(), "Failed");
        logger.Error(Thrown(() => new TimeoutException("no answer")), "Failed again");

        reader.Sum(SecurityEventMetrics.ProtectionFailuresName).Should().Be(1, "only the exception that could not be read is counted");
    }

    [Fact]
    public async Task Configure_Should_PutEverySinkBehindTheExceptionProtection_When_AHostLogsAnException()
    {
        // The host's own pipeline (ArivaLogging.Configure), through the vm-local file sink, which writes what the console would.
        var folder = Directory.CreateTempSubdirectory("ariva-arv212-").FullName;
        try
        {
            var file = Path.Combine(folder, "host.log");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { [ArivaLogging.FileSetting] = file })
                .Build();
            var secret = IntegrationCredentials.NewSecret();
            var seed = Base32.Encode(RandomNumberGenerator.GetBytes(20));

            await using (var logger = ArivaLogging.Configure(new LoggerConfiguration(), configuration, ArivaEnvironment.VmLocal).CreateLogger())
                logger.Error(Thrown(() => new InvalidOperationException($"client {secret} with seed {seed} refused")), "Integration call failed");

            var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Holds(text, secret[4..]).Should().BeFalse("the file sink writes the exception without the secret");
            Holds(text, seed).Should().BeFalse("nor the seed");
            Holds(text, "Integration call failed").Should().BeTrue("the line is written");
            Holds(text, "System.InvalidOperationException").Should().BeTrue("with the original type's name");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    #endregion
}
