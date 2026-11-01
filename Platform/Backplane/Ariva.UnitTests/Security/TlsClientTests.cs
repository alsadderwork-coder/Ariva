using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using Ariva.Di.Extensions;
using Ariva.Infra.Caching;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.NHibernate.Drivers;
using Ariva.Infra.Notifications;
using Ariva.Infra.Security;
using Ariva.Infra.Settings;
using Ariva.ServiceDefaults;
using Ariva.UnitTests.Security.Fixtures;
using Confluent.Kafka;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Npgsql;
using StackExchange.Redis;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-280 (ASVS 5.0 V12.1.1, V12.1.2): every TLS client a host opens, beyond the outbound integration calls (ARV-205,
/// <c>OutboundTests</c>), offers TLS 1.2 and 1.3 only and, on Linux, the pods' suites only (<see cref="ModernTls"/>): PostgreSQL
/// (Npgsql through <see cref="PostgresConnections"/>, NHibernate's driver too), the SMTP relay (<see cref="SmtpRelayClient"/>, TLS
/// on connect and STARTTLS), Redis (<see cref="RedisSettings.Options"/>), Kafka (librdkafka through <see cref="KafkaClientConfig"/>
/// and MassTransit's rider through <see cref="MessagingExtensions.ConfigureRider"/>) and the log and telemetry collectors
/// (<see cref="CollectorTrust.Pin"/>). Each is read twice: from the options it hands its handshake, and from the ClientHello it
/// sends a listener that answers nothing (<see cref="HelloCatcher"/>). On Linux, a control per library shows its own default
/// offering a suite outside the list, so each capture's silence is Ariva's setting, not the platform's.
/// </summary>
public sealed class TlsClientTests
{
    #region Fields

    private static readonly TimeSpan HelloWait = TimeSpan.FromSeconds(15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Tests

    [Fact]
    public void CollectorTls_Should_EqualModernTls_When_Compared()
    {
        // Ariva.ServiceDefaults references no Ariva project (ARV-066), so its copy is held to the original here.
        CollectorTls.ProtocolNames.Should().Equal(ModernTls.ProtocolNames);
        CollectorTls.Protocols.Should().Be(ModernTls.Protocols);
        CollectorTls.CipherSuites.Should().Equal(ModernTls.CipherSuites);
        if (OperatingSystem.IsLinux())
            CollectorTls.CipherSuitesPolicy.AllowedCipherSuites.Should().Equal(ModernTls.CipherSuites);
        else
            CollectorTls.CipherSuitesPolicy.Should().BeNull();
    }

    [Fact]
    public void ModernTls_Should_NameTheSameTls12SuitesForOpenSsl_When_KafkaTakesThem()
    {
        // librdkafka takes OpenSSL's names: the six TLS 1.2 suites of the list, in its order, and security level 2.
        var openSsl = ModernTls.OpenSslCipherList.Split(':');
        openSsl[^1].Should().Be("@SECLEVEL=2", "no version below TLS 1.2 at security level 2, whatever the image's OpenSSL configuration");
        string[] expected =
        [
            "ECDHE-ECDSA-AES128-GCM-SHA256", "ECDHE-ECDSA-AES256-GCM-SHA384", "ECDHE-ECDSA-CHACHA20-POLY1305",
            "ECDHE-RSA-AES128-GCM-SHA256", "ECDHE-RSA-AES256-GCM-SHA384", "ECDHE-RSA-CHACHA20-POLY1305"
        ];
        openSsl[..^1].Should().Equal(expected);
        ModernTls.CipherSuites.Skip(3).Select(suite => suite.ToString()).Should().Equal(
            "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256", "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384", "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256",
            "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256", "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384", "TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256");
    }

    [Fact]
    public void PostgreSql_Should_PinTheVersionsAndSuites_When_ArivaOrNHibernateMakesTheConnection()
    {
        using var made = PostgresConnections.Create("Host=localhost");
        using var driven = (NpgsqlConnection)new NHibernatePostgresClientDriver().CreateConnection();

        foreach (var connection in new[] { made, driven })
        {
            var options = new SslClientAuthenticationOptions();
            connection.SslClientAuthenticationOptionsCallback(options);
            ShouldBeModern(options);
            options.CertificateChainPolicy.DisableCertificateDownloads.Should().BeTrue("ARV-226 still holds");
        }
    }

    [Fact]
    public async Task PostgreSql_Should_OfferOnlyModernTls_When_AConnectionOpens()
    {
        await using var catcher = HelloCatcher.Start(HelloPreamble.Postgres);
        await using (var connection = PostgresConnections.Create(Database(catcher.Port)))
        {
            var open = () => connection.OpenAsync(Ct);
            await open.Should().ThrowAsync<Exception>("the listener closes after the hello");
        }

        (await catcher.FirstHelloAsync(HelloWait, Ct)).ShouldOfferOnlyModernTls("PostgreSQL");
        if (OperatingSystem.IsLinux())
        {
            // The control: a connection with Npgsql's own handshake options offers more.
            await using var control = HelloCatcher.Start(HelloPreamble.Postgres);
            await using var plain = new NpgsqlConnection(Database(control.Port));
            var open = () => plain.OpenAsync(Ct);
            await open.Should().ThrowAsync<Exception>();
            ShouldOfferAnotherSuite(await control.FirstHelloAsync(HelloWait, Ct), "Npgsql's own handshake");
        }
    }

    [Theory]
    [InlineData("SslOnConnect")]
    [InlineData("StartTls")]
    public async Task Smtp_Should_OfferOnlyModernTls_When_TheRelayIsReached(string security)
    {
        using (var client = new SmtpRelayClient())
        {
            var options = (SslClientAuthenticationOptions)typeof(SmtpRelayClient)
                .GetMethod("GetSslClientAuthenticationOptions", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(client, ["localhost", new RemoteCertificateValidationCallback((_, _, _, _) => false)]);
            ShouldBeModern(options);
        }

        var preamble = security == "StartTls" ? HelloPreamble.SmtpStartTls : HelloPreamble.None;
        await using var catcher = HelloCatcher.Start(preamble);
        var transport = new MailKitTransport(new EmailSettings { Enabled = true, FromAddress = "ariva@example.com" },
            new SmtpSettings { Host = "localhost", Port = catcher.Port, Security = security, TimeoutSeconds = 5 });

        var batch = await transport.SendAsync([new OutgoingEmail(Guid.NewGuid(), "duty@example.com", "Probe", "Probe")], Ct);

        (batch.Failures.Count + batch.NotTried.Count).Should().Be(1, "the listener closes after the hello");
        (await catcher.FirstHelloAsync(HelloWait, Ct)).ShouldOfferOnlyModernTls($"the SMTP relay client ({security})");
        if (OperatingSystem.IsLinux())
        {
            // The control: MailKit's own client offers more.
            await using var control = HelloCatcher.Start(preamble);
            using var own = new MailKit.Net.Smtp.SmtpClient { Timeout = 5_000 };
            var connect = () => own.ConnectAsync("localhost", control.Port, Enum.Parse<MailKit.Security.SecureSocketOptions>(security), Ct);
            await connect.Should().ThrowAsync<Exception>();
            ShouldOfferAnotherSuite(await control.FirstHelloAsync(HelloWait, Ct), "MailKit's own client");
        }
    }

    [Theory]
    [InlineData("", "Tls12, Tls13")]
    [InlineData(",sslProtocols=None", "Tls12, Tls13")]
    [InlineData(",sslProtocols=Tls12", "Tls12")]
    [InlineData(",sslProtocols=Tls13", "Tls13")]
    [InlineData(",sslProtocols=Tls12|Tls13", "Tls12, Tls13")]
    [InlineData(",sslProtocols=Tls11|Tls12", "Tls12")]
    public void Redis_Should_OfferTls12And13OrFewer_When_TheConnectionStringNamesItsVersions(string named, string offered)
    {
        var options = new RedisSettings { ConnectionString = $"redis:6379,ssl=true{named}" }.Options().SslClientAuthenticationOptions("redis");

        options.EnabledSslProtocols.Should().Be(Enum.Parse<SslProtocols>(offered), "sslProtocols narrows TLS 1.2 and 1.3, never widens them");
        if (OperatingSystem.IsLinux())
            options.CipherSuitesPolicy.AllowedCipherSuites.Should().Equal(ModernTls.CipherSuites);
        else
            options.CipherSuitesPolicy.Should().BeNull();
        options.CertificateChainPolicy.DisableCertificateDownloads.Should().BeTrue("ARV-226 still holds");
    }

    [Fact]
    public void Redis_Should_RefuseTheConnectionString_When_ItsSslProtocolsNamesNoVersionArivaSpeaks()
    {
        var settings = new RedisSettings { Enabled = true, ConnectionString = "redis-tls-probe:6379,ssl=true,sslProtocols=Tls11", CaCertificatePath = "/ca.crt" };

        var build = () => settings.Options();

        build.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("sslProtocols").And.NotContain("redis-tls-probe",
            "the message names no part of the connection string");
        settings.ProductionTransportProblem(_ => true).Should().Contain("sslProtocols", "k8s-prd refuses it at start too");
        new RedisSettings { Enabled = true, ConnectionString = "redis:6379,ssl=true,sslProtocols=Tls12", CaCertificatePath = "/ca.crt" }
            .ProductionTransportProblem(_ => true).Should().BeNull();
        new RedisSettings { ConnectionString = "redis:6379,sslProtocols=Tls11" }.Options().SslClientAuthenticationOptions
            .Should().BeNull("without TLS there is no handshake to refuse");
    }

    [Fact]
    public async Task Redis_Should_OfferOnlyModernTls_When_ItConnects()
    {
        await using var catcher = HelloCatcher.Start();
        var settings = new RedisSettings { ConnectionString = $"localhost:{catcher.Port},ssl=true,connectTimeout=2000" };
        await using (await ConnectionMultiplexer.ConnectAsync(settings.Options()))
            (await catcher.FirstHelloAsync(HelloWait, Ct)).ShouldOfferOnlyModernTls("Redis");

        if (OperatingSystem.IsLinux())
        {
            // The control: StackExchange.Redis's own handshake offers more.
            await using var control = HelloCatcher.Start();
            var own = ConfigurationOptions.Parse($"localhost:{control.Port},ssl=true,connectTimeout=2000");
            own.AbortOnConnectFail = false;
            await using (await ConnectionMultiplexer.ConnectAsync(own))
                ShouldOfferAnotherSuite(await control.FirstHelloAsync(HelloWait, Ct), "StackExchange.Redis's own handshake");
        }
    }

    [Theory]
    [InlineData("Ssl", true)]
    [InlineData("SaslSsl", true)]
    [InlineData("Plaintext", false)]
    [InlineData("SaslPlaintext", false)]
    public void Kafka_Should_PinTheSuitesOnEveryOwnClient_When_TheProtocolSpeaksTls(string protocol, bool tls)
    {
        var settings = new KafkaSettings { SecurityProtocol = protocol, SaslMechanism = protocol.StartsWith("Sasl", StringComparison.Ordinal) ? "ScramSha512" : null };
        var expected = tls ? ModernTls.OpenSslCipherList : null;

        KafkaClientConfig.UsesTls(settings).Should().Be(tls);
        KafkaClientConfig.Admin(settings).SslCipherSuites.Should().Be(expected);
        KafkaClientConfig.Producer(settings).SslCipherSuites.Should().Be(expected);
        KafkaClientConfig.Consumer(settings, "ariva-test.tls").SslCipherSuites.Should().Be(expected);
    }

    [Theory]
    [InlineData("Ssl", null, true, true)]
    [InlineData("SaslSsl", "/ca.crt", true, true)]
    [InlineData("Plaintext", "/ca.crt", true, false)]
    [InlineData("Plaintext", null, false, false)]
    public void KafkaRider_Should_PinTheSuitesOnItsHost_When_TheProtocolSpeaksTls(string protocol, string caLocation, bool ssl, bool suites)
    {
        var settings = new KafkaSettings { SecurityProtocol = protocol, SslCaLocation = caLocation };
        var host = new Mock<IKafkaHostConfigurator>();
        var configured = new Mock<IKafkaSslConfigurator>();
        string cipherSuites = null, ca = null;
        configured.SetupSet(c => c.CipherSuites = It.IsAny<string>()).Callback<string>(value => cipherSuites = value);
        configured.SetupSet(c => c.CaLocation = It.IsAny<string>()).Callback<string>(value => ca = value);
        host.Setup(h => h.UseSsl(It.IsAny<Action<IKafkaSslConfigurator>>())).Callback<Action<IKafkaSslConfigurator>>(configure => configure(configured.Object));

        MessagingExtensions.ConfigureRiderHost(host.Object, settings);

        host.Verify(h => h.UseSsl(It.IsAny<Action<IKafkaSslConfigurator>>()), ssl ? Times.Once() : Times.Never());
        cipherSuites.Should().Be(suites ? ModernTls.OpenSslCipherList : null);
        ca.Should().Be(ssl ? caLocation : null);
    }

    [Fact]
    public async Task Kafka_Should_OfferOnlyModernTls_When_ArivasOwnClientReachesABroker()
    {
        await using var catcher = HelloCatcher.Start();
        var settings = new KafkaSettings { BootstrapServers = $"127.0.0.1:{catcher.Port}", SecurityProtocol = "Ssl" };
        using (var admin = Quiet(new AdminClientBuilder(KafkaClientConfig.Admin(settings))).Build())
        {
            var metadata = () => admin.GetMetadata(TimeSpan.FromSeconds(3));
            metadata.Should().Throw<KafkaException>("the listener closes after the hello");
        }

        (await catcher.FirstHelloAsync(HelloWait, Ct)).ShouldOfferOnlyModernTls("librdkafka (Ariva's own clients)");
        if (OperatingSystem.IsLinux())
        {
            // The control: librdkafka's own suites, without Ariva's list.
            await using var control = HelloCatcher.Start();
            using var own = Quiet(new AdminClientBuilder(new AdminClientConfig { BootstrapServers = $"127.0.0.1:{control.Port}", SecurityProtocol = SecurityProtocol.Ssl }))
                .Build();
            var metadata = () => own.GetMetadata(TimeSpan.FromSeconds(3));
            metadata.Should().Throw<KafkaException>();
            ShouldOfferAnotherSuite(await control.FirstHelloAsync(HelloWait, Ct), "librdkafka's own suites");
        }
    }

    [Fact]
    public async Task Kafka_Should_OfferNoVersionBelowTls12_When_OnlyItsSecurityLevelHoldsThemBack()
    {
        // librdkafka has no minimum version setting: OpenSSL's security level is what leaves TLS 1.0 and 1.1 out. The same list at
        // level 0, as an image's OpenSSL configuration could set it, offers both (measured: 0x0304, 0x0303, 0x0302, 0x0301), so
        // the level in Ariva's list (the capture test above: 1.2 and 1.3 only) is what holds them back. This control depends on the
        // OpenSSL that librdkafka.redist bundles still building TLS 1.0 and 1.1: if it fails after a package upgrade, measure again
        // (the pin may no longer be the only floor); it is not a regression of Ariva's list.
        var levelZero = ModernTls.OpenSslCipherList.Replace("@SECLEVEL=2", "@SECLEVEL=0", StringComparison.Ordinal);
        levelZero.Should().NotBe(ModernTls.OpenSslCipherList);
        await using var control = HelloCatcher.Start();
        using (var admin = Quiet(new AdminClientBuilder(new AdminClientConfig
               {
                   BootstrapServers = $"127.0.0.1:{control.Port}", SecurityProtocol = SecurityProtocol.Ssl, SslCipherSuites = levelZero
               })).Build())
        {
            var metadata = () => admin.GetMetadata(TimeSpan.FromSeconds(3));
            metadata.Should().Throw<KafkaException>();
        }

        (await control.FirstHelloAsync(HelloWait, Ct)).SupportedVersions.Should().Contain([(ushort)0x0301, (ushort)0x0302],
            "at security level 0 librdkafka's OpenSSL offers TLS 1.0 and 1.1");
    }

    [Fact]
    public async Task KafkaRider_Should_OfferOnlyModernTls_When_ATopicEndpointReachesABroker()
    {
        // MassTransit's rider, configured as every host configures it, merges its host's TLS settings into each topic endpoint's
        // consumer: the endpoint's ClientHello is read, so the merge itself is under test, not only the configurator's calls.
        await using var catcher = HelloCatcher.Start();
        var settings = new KafkaSettings { BootstrapServers = $"127.0.0.1:{catcher.Port}", SecurityProtocol = "Ssl", ServiceName = "tls-probe" };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMassTransit(bus =>
        {
            bus.DisableUsageTelemetry();
            bus.UsingInMemory();
            bus.AddRider(rider => rider.UsingKafka(KafkaClientConfig.Rider(), (context, kafka) => MessagingExtensions.ConfigureRider(context, kafka, settings)));
        });
        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBusControl>();
        await bus.StartAsync(Ct);
        try
        {
            var endpoint = provider.GetRequiredService<IKafkaRider>().ConnectTopicEndpoint<string, TlsProbe>("ariva.tls-probe.v1", "ariva-tls-probe.hello",
                (_, configurator) => configurator.Handler<TlsProbe>(_ => Task.CompletedTask));

            (await catcher.FirstHelloAsync(HelloWait, Ct)).ShouldOfferOnlyModernTls("MassTransit's rider (a topic endpoint's consumer)");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await endpoint.StopAsync(stop.Token).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await bus.StopAsync(stop.Token).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    [Fact]
    public async Task Collectors_Should_OfferOnlyModernTls_When_APinnedClientReachesOne()
    {
        var services = new ServiceCollection();
        CollectorTrust.Pin(services, null, [CollectorTrust.LokiClient, CollectorTrust.OtlpLogsClient, CollectorTrust.OtlpExporterClient]);
        services.AddHttpClient("plain");
        await using var provider = services.BuildServiceProvider();

        foreach (var name in new[] { CollectorTrust.LokiClient, CollectorTrust.OtlpLogsClient, CollectorTrust.OtlpExporterClient })
        {
            var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
            while (handler is DelegatingHandler delegating)
                handler = delegating.InnerHandler;
            var sockets = handler.Should().BeOfType<SocketsHttpHandler>().Subject;
            ShouldBeModern(sockets.SslOptions);
        }

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        await using (var catcher = HelloCatcher.Start())
        {
            using var pinned = factory.CreateClient(CollectorTrust.LokiClient);
            var send = () => pinned.GetAsync(new Uri($"https://localhost:{catcher.Port}/loki/api/v1/push"), Ct);
            await send.Should().ThrowAsync<HttpRequestException>("the listener closes after the hello");
            (await catcher.FirstHelloAsync(HelloWait, Ct)).ShouldOfferOnlyModernTls("a collector client");
        }

        if (OperatingSystem.IsLinux())
        {
            // The control: the factory's default handler offers more.
            await using var control = HelloCatcher.Start();
            using var plain = factory.CreateClient("plain");
            var send = () => plain.GetAsync(new Uri($"https://localhost:{control.Port}/"), Ct);
            await send.Should().ThrowAsync<HttpRequestException>();
            ShouldOfferAnotherSuite(await control.FirstHelloAsync(HelloWait, Ct), "the factory's default handler");
        }
    }

    #endregion

    #region Methods

    /// <summary>A connection string for a server on loopback <paramref name="port"/> that requires TLS (the listener never answers it).</summary>
    private static string Database(int port) => new NpgsqlConnectionStringBuilder
    {
        Host = "localhost",
        Port = port,
        Database = "ariva",
        Username = "ariva",
        SslMode = SslMode.Require,
        Timeout = 5,
        Pooling = false
    }.ConnectionString;

    /// <summary>Options that name TLS 1.2 and 1.3 exactly and, on Linux, the pods' suites exactly.</summary>
    private static void ShouldBeModern(SslClientAuthenticationOptions options)
    {
        options.EnabledSslProtocols.Should().Be(ModernTls.Protocols, "TLS 1.2 and 1.3 only");
        if (OperatingSystem.IsLinux())
            options.CipherSuitesPolicy.AllowedCipherSuites.Should().Equal(ModernTls.CipherSuites, "on Linux the pods' suites alone");
        else
            options.CipherSuitesPolicy.Should().BeNull("only Linux restricts suites per connection; elsewhere the platform's TLS configuration decides");
    }

    /// <summary>The control: a library's own handshake offers a suite outside the pods' list.</summary>
    private static void ShouldOfferAnotherSuite(ClientHello hello, string control)
    {
        var listed = ModernTls.CipherSuites.Select(suite => (ushort)suite).Append(ClientHello.RenegotiationScsv).ToHashSet();
        hello.CipherSuites.Should().Contain(suite => !listed.Contains(suite), "{0} offers a suite outside the list, so the pin is what removes it", control);
    }

    /// <summary>An admin client builder whose librdkafka log and errors go nowhere (the listener refuses every handshake).</summary>
    private static AdminClientBuilder Quiet(AdminClientBuilder builder) => builder.SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { });

    #endregion

    #region Types

    /// <summary>The message the probe endpoint would consume; it never receives one.</summary>
    public sealed record TlsProbe(string Text);

    #endregion
}
