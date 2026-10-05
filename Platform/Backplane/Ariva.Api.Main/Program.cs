using Ariva.Api.Common.Hubs;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Common.Filters;
using Ariva.Api.Common.Security;
using Ariva.Di.Extensions;
using Ariva.Api.Common.HealthChecks;
using Ariva.Api.Common.Hosting;
using Ariva.Di;

// Ariva.Api.Main: configuration and operations REST API, and later the SignalR live hub.
// The regions follow AMAN's Program.cs order. Calls named in comments are AMAN extensions that backlog
// stories port into Ariva.Api.Common; until then this file calls framework APIs, RegisterArivaServices,
// the security baseline and MapArivaHealthChecks only, so it builds without NuGet packages.

#region Configuration

// AMAN: Util.GlobalDisableReloadOnChange(); var environment = Util.GetOrCreateEnvironment();
//       builder.Configuration.AddAppConfiguration(environment);
// Until that story lands, the layering is done here with framework APIs only:
// appsettings.base.json, appsettings.base.<env>.json, appsettings.service.json, appsettings.service.<env>.json,
// then environment variables and command line arguments, which keep the highest precedence.
// The environment is also the host environment name, so error handling sees the same value as the files.
// --migrate (ARV-006) is handled below and never reaches the configuration command line provider.
var migrate = DatabaseMigration.IsRequested(args);
args = DatabaseMigration.WithoutFlag(args);
// --provision-topics (ARV-062): the Helm topics Job's command, never configuration.
var provisionTopics = TopicProvisioning.IsRequested(args);
args = TopicProvisioning.WithoutFlag(args);
// --create-break-glass / --rotate-break-glass (ARV-010c) are installer commands, never configuration.
var breakGlassArgs = args;
var breakGlass = BreakGlassCommand.IsRequested(args);
args = BreakGlassCommand.WithoutFlags(args);

var environment = ArivaEnvironment.Resolve(args, AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    EnvironmentName = environment
});

builder.Configuration
    .AddJsonFile("appsettings.base.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.base.{environment}.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.service.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.service.{environment}.json", optional: true, reloadOnChange: false)
    // Local development only: written by scripts/dev-up.mjs from .env; git-ignored and never in images.
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

#endregion

#region Migration and topic jobs

// One-shot schema migration: the Helm migration Job starts this image with --migrate before each release.
if (migrate)
{
    return await DatabaseMigration.RunAsync(builder.Configuration);
}

// One-shot topic provisioning: the Helm topics Job starts this image with --provision-topics before each release.
if (provisionTopics)
{
    return await TopicProvisioning.RunAsync(builder.Configuration);
}

// Installer command: the deployment's break-glass account, printed once (ARV-010c).
if (breakGlass)
{
    return await BreakGlassCommand.RunAsync(builder.Configuration, builder.Environment.EnvironmentName, breakGlassArgs, Console.Out);
}

#endregion

#region Logging, Hosting and Telemetry

// Serilog with secret redaction, JSON console, Loki and OTLP by configuration; traces and metrics over OTLP (ARV-007).
builder.AddAppLogging();
builder.AddAppServiceDefaults();
// AMAN: builder.Host.AddAppHosting(); builder.WebHost.AddAppWebHost(...);

#endregion

#region Services

// Main consumes device health reports into heartbeats (ARV-025).
builder.Services.RegisterArivaServices(builder.Configuration, messaging => messaging.ConsumeDeviceHealth());

// Security baseline (docs/security/cwe-controls.md): default deny (fallback policy, Ariva.Deny answers challenges),
// ES256 access tokens from Ariva.Api.Main with permissions from stored grants (ARV-010a), input limits, rate limiting,
// ProblemDetails errors, trusted forwarded headers and the CORS allow-list.
builder.Services.AddAppSecurityBaseline(builder.Configuration, builder.Environment.EnvironmentName);
builder.Services.AddAppControllers();
// OpenAPI document for the dynamic scan (ARV-063): only with OpenApi:Enabled, administrators only, refused in k8s-prd.
builder.Services.AddArivaOpenApi(builder.Configuration, builder.Environment.EnvironmentName);

// Ariva.Api.Main signs users in and issues tokens (ADR-0026); the other hosts only validate them.
builder.Services.AddArivaTokenIssuing(builder.Configuration, builder.Environment.EnvironmentName);

// Integration clients (ARV-042): administrators register the systems that call the Integration API.
builder.Services.AddArivaIntegrationClients();
builder.Services.AddArivaOutboundEndpoints(builder.Configuration, builder.Environment.EnvironmentName);
builder.Services.AddArivaFlightSchedules(builder.Configuration);
builder.Services.AddArivaArrivalWave(builder.Configuration);

// Demo Airport (DMO) topology and zone profile v12 in vm-local and k8s-demo; refused outside dev and demo (ARV-019).
builder.Services.AddArivaDemoSeed(builder.Configuration, builder.Environment.EnvironmentName);

// Device heartbeats (ARV-025): a commissioned device not heard from for Devices:Health:HeartbeatTimeoutSeconds goes
// Offline and its queue zone Degraded; one sweep at a time across replicas.
builder.Services.AddArivaDeviceHealthMonitor();

// AMAN: AddAppCaching, AddAppHealthChecks, AddAppRouting, AddAppOpenApi, AddAppSignalR.
// SignalR (ARV-035): the live queue hub with session checks (ADR-0026), MessagePack beside JSON, bounded messages, and
// the Redis backplane when Redis is configured; the relay forwards Stream's live snapshots to the zone groups.
var signalR = builder.Services.AddSignalR(options =>
    {
        options.MaximumReceiveMessageSize = 8 * 1024;
        options.EnableDetailedErrors = false;
        options.MaximumParallelInvocationsPerClient = 1;
    })
    .AddMessagePackProtocol()
    .AddArivaHubSessions();
var redisSettings = Ariva.Infra.Caching.RedisSettings.From(builder.Configuration);
if (redisSettings.Enabled)
    signalR.AddStackExchangeRedis(redisSettings.ConnectionString, options => options.Configuration.ChannelPrefix =
        StackExchange.Redis.RedisChannel.Literal(redisSettings.InstanceName + "signalr"));
builder.Services.AddScoped<Ariva.Infra.Live.LiveZoneDirectory>();
builder.Services.AddHostedService<Ariva.Api.Main.Hubs.LiveRelay>();

#endregion

var app = builder.Build();

#region Middlewares

// AMAN order (UseAppForwardedHeaders, UseErrorHandling, UseRouting, UseRequestTracking, UseAppCors,
// UseAuthentication, UseSessionContext, UseAuthorization) with the security headers and rate limiting added.
app.UseAppForwardedHeaders();
app.UseAppSecurityHeaders();
app.UseErrorHandling();
app.UseRouting();
// AMAN: UseRequestTracking
app.UseAppCors();
app.UseAppRateLimiting();
app.UseAuthentication();
app.UseSessionValidation();
app.UsePendingScope();
// AMAN: UseSessionContext
app.UseAuthorization();

#endregion

#region Endpoints

app.MapArivaHealthChecks();
app.MapControllers();
app.MapArivaOpenApi(app.Configuration);
// The hub's [Permission] and RequireAuthorization both apply, so the hub stays closed even without its attribute.
app.MapHub<Ariva.Api.Main.Hubs.LiveHub>(Ariva.Api.Main.Hubs.LiveHub.Path, Ariva.Api.Main.Hubs.LiveHub.Configure).RequireAuthorization();

// AMAN: MapOpenApi, MapScalarApiReference. Controllers live in Controllers/AdminArea/<Entity>/Controller.cs
// and Controllers/OpsArea/<Entity>/Controller.cs; hubs are mapped here with MapHub and RequireAuthorization.

#endregion

#region Bootstrap

// AMAN: await IBootstrapper.Initialize();

#endregion

await app.RunAsync();
return 0;
