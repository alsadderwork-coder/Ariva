using System.Threading.RateLimiting;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Emulators.Aodb;
using Ariva.Simulation.Api.Emulators.Integration;
using Ariva.Simulation.Api.Scenarios;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Ariva.Simulation.Api.Emulators;

/// <summary>The three emulated systems' Ariva integration clients (AMAN, the mock immigration system, the AODB).</summary>
public sealed class FeedClients(ArivaIntegrationClient aman, ArivaIntegrationClient immigration, ArivaIntegrationClient aodb)
{
    public ArivaIntegrationClient Aman { get; } = aman;
    public ArivaIntegrationClient Immigration { get; } = immigration;
    public ArivaIntegrationClient Aodb { get; } = aodb;
}

/// <summary>The emulated border systems: AMAN and the mock immigration system.</summary>
public sealed class BorderFeeds(ImmigrationFeedEmulator aman, ImmigrationFeedEmulator immigration)
{
    public ImmigrationFeedEmulator Aman { get; } = aman;
    public ImmigrationFeedEmulator Immigration { get; } = immigration;
}

/// <summary>
/// Registers the AODB, AMAN and immigration emulators (ARV-029): settings validated at start, the Ariva Integration API
/// client (no redirects, cookies or environment proxy, so a token goes to Ariva and nowhere else), the shared feed time,
/// the three emulators as sinks of the demo clock, and the mock partners' authentication (the mock AMAN's tokens, the
/// AODB's ACRIS key) with the exchange's per-address limit.
/// </summary>
public static class FeedEmulatorExtensions
{
    public const string HttpClientName = "ariva-integration";

    public static IServiceCollection AddFeedEmulators(this IServiceCollection services, IConfiguration configuration)
    {
        Bind<ArivaTargetSettings>(services, configuration, ArivaTargetSettings.Section);
        Bind<AmanEmulatorSettings>(services, configuration, AmanEmulatorSettings.Section);
        Bind<ImmigrationEmulatorSettings>(services, configuration, ImmigrationEmulatorSettings.Section);
        Bind<AodbEmulatorSettings>(services, configuration, AodbEmulatorSettings.Section);

        services
            .AddHttpClient(HttpClientName, (provider, client) =>
            {
                var target = provider.GetRequiredService<IOptionsMonitor<ArivaTargetSettings>>().CurrentValue;
                if (!string.IsNullOrEmpty(target.IntegrationUrl))
                    client.BaseAddress = new Uri(target.IntegrationUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(target.RequestTimeoutSeconds);
                client.MaxResponseContentBufferSize = 1024 * 1024;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
            .RemoveAllLoggers();

        services.AddSingleton<FeedTime>();
        services.AddSingleton<AmanFeedBuffer>();
        services.AddSingleton(provider => new AmanKafka(() => provider.GetRequiredService<IOptionsMonitor<AmanEmulatorSettings>>().CurrentValue.Kafka,
            provider.GetRequiredService<ILogger<AmanKafka>>()));
        services.AddSingleton<MockAmanTokens>();
        services.AddSingleton(provider =>
        {
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            var time = provider.GetRequiredService<TimeProvider>();
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Ariva.Simulation.Feeds");
            ArivaIntegrationClient Client(string name, IntegrationClientSettings credentials)
            {
                var client = new ArivaIntegrationClient(name, () => factory.CreateClient(HttpClientName), time, logger);
                client.Use(credentials);
                return client;
            }

            return new FeedClients(
                Client("aman", provider.GetRequiredService<IOptionsMonitor<AmanEmulatorSettings>>().CurrentValue.Client),
                Client("immigration", provider.GetRequiredService<IOptionsMonitor<ImmigrationEmulatorSettings>>().CurrentValue.Client),
                Client("aodb", provider.GetRequiredService<IOptionsMonitor<AodbEmulatorSettings>>().CurrentValue.Client));
        });
        services.AddSingleton(provider =>
        {
            var engine = provider.GetRequiredService<ScenarioEngine>();
            var time = provider.GetRequiredService<FeedTime>();
            var clients = provider.GetRequiredService<FeedClients>();
            var target = provider.GetRequiredService<IOptionsMonitor<ArivaTargetSettings>>();
            var aman = provider.GetRequiredService<IOptionsMonitor<AmanEmulatorSettings>>();
            var immigration = provider.GetRequiredService<IOptionsMonitor<ImmigrationEmulatorSettings>>();
            var loggers = provider.GetRequiredService<ILoggerFactory>();
            return new BorderFeeds(
                new ImmigrationFeedEmulator("aman", engine, time, () => (aman.CurrentValue.SiteCode, aman.CurrentValue.Sides, aman.CurrentValue.RetainMinutes),
                    () => target.CurrentValue.SiteCode, clients.Aman, provider.GetRequiredService<AmanKafka>(), provider.GetRequiredService<AmanFeedBuffer>(),
                    loggers.CreateLogger("Ariva.Simulation.Aman")),
                new ImmigrationFeedEmulator("immigration", engine, time, () => (immigration.CurrentValue.SiteCode, immigration.CurrentValue.Sides, 0),
                    () => target.CurrentValue.SiteCode, clients.Immigration, kafka: null, buffer: null, loggers.CreateLogger("Ariva.Simulation.Immigration")));
        });
        services.AddSingleton(provider => new AodbEmulator(provider.GetRequiredService<ScenarioEngine>(), provider.GetRequiredService<FeedTime>(),
            provider.GetRequiredService<IOptionsMonitor<AodbEmulatorSettings>>(), provider.GetRequiredService<IOptionsMonitor<ArivaTargetSettings>>(),
            provider.GetRequiredService<FeedClients>().Aodb, provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<AodbEmulator>>()));
        services.AddSingleton<IDemoMinuteSink>(provider => provider.GetRequiredService<BorderFeeds>().Aman);
        services.AddSingleton<IDemoMinuteSink>(provider => provider.GetRequiredService<BorderFeeds>().Immigration);
        services.AddSingleton<IDemoMinuteSink>(provider => provider.GetRequiredService<AodbEmulator>());

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, MockAmanTokenHandler>(MockPartnerSchemes.AmanToken, displayName: null, configureOptions: null)
            .AddScheme<AuthenticationSchemeOptions, MockAodbKeyHandler>(MockPartnerSchemes.AodbKey, displayName: null, configureOptions: null);
        services.AddAuthorizationBuilder()
            .AddPolicy(MockPartnerSchemes.AmanPolicy, policy => policy.AddAuthenticationSchemes(MockPartnerSchemes.AmanToken).RequireAuthenticatedUser())
            .AddPolicy(MockPartnerSchemes.AodbPolicy, policy => policy.AddAuthenticationSchemes(MockPartnerSchemes.AodbKey).RequireAuthenticatedUser());
        services.AddRateLimiter(limiter => limiter.AddPolicy(MockPartnerSchemes.AmanAuthLimit, context =>
        {
            var perMinute = context.RequestServices.GetRequiredService<IOptionsMonitor<AmanEmulatorSettings>>().CurrentValue.Mock.AuthPerMinute;
            return RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        }));
        return services;
    }

    private static void Bind<T>(IServiceCollection services, IConfiguration configuration, string section) where T : class =>
        services.AddOptions<T>().Bind(configuration.GetSection(section)).ValidateDataAnnotations().ValidateOnStart();
}
